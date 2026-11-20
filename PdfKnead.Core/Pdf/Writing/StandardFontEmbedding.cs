using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// A standard font that is not embedded (Helvetica, Times, Courier and Arial, each with its styles, Symbol, ZapfDingbats), embedded as the bundled font the
/// display draws it with (<see cref="StandardFonts"/>, metrically the same) — PDF/A wants every font that draws visible text embedded
/// (ISO 19005-2, 6.2.11.4.1). Written as a TrueType font in the way 6.2.11.6 asks: an encoding based on WinAnsiEncoding with glyph names of the
/// Adobe Glyph List, a (3,1) cmap in the font program (a subset of the glyphs the encoding reaches), widths from the font program (6.2.11.5).
/// Only where the page then looks the same: every code the text shows finds its glyph, and its width given in the file differs from the font
/// program's by at most one unit (then the file's stays, the text does not move) — otherwise the font is left as it is and the check names it.
/// Codes no text shows get the font program's width and lose names the font cannot serve; a font no text shows is not embedded (PDF/A does
/// not ask for it).
/// </summary>
internal static class StandardFontEmbedding
{
    /// <summary>A font ready to be embedded.</summary>
    /// <param name="Font">The new font dictionary, without /FontDescriptor (the writer refers to <paramref name="Descriptor"/>) and /ToUnicode.</param>
    /// <param name="Descriptor">The font descriptor, without the font program (the writer refers to <paramref name="Program"/>).</param>
    /// <param name="Program">The font program (a subset).</param>
    /// <param name="Key">Equal for fonts that can share descriptor and program.</param>
    /// <param name="ProgramKey">The key of the descriptor for the program (FontFile2 for TrueType, FontFile3 for a compact font).</param>
    /// <param name="ToUnicode">What the codes read as, where the glyph names alone do not say it for every reader (ZapfDingbats); null if not needed.</param>
    public sealed record Result(PdfDictionary Font, PdfDictionary Descriptor, PdfStream Program, string Key, string ProgramKey = "FontFile2",
        PdfStream? ToUnicode = null);

    /// <summary>The replacement for a font dictionary; null if it is no standard font that is not embedded, or the page would not look the same.</summary>
    /// <param name="font">The font dictionary (a simple font).</param>
    /// <param name="encoding">Its /Encoding, resolved (a name or a dictionary whose /Differences resolves); null if it has none.</param>
    /// <param name="widths">Its /Widths, resolved; null if it has none.</param>
    /// <param name="descriptor">Its /FontDescriptor, resolved; null if it has none.</param>
    /// <param name="used">The codes text is shown with (<see cref="PdfACheck.Used"/>); null if none.</param>
    /// <param name="drawn">Per code: the glyph of the substitute the display draws (-1: none), the width it sets the code with (1/1000 em) and the
    /// text it reads; null if not known (then a font with codes shown without a glyph is not embedded).</param>
    /// <param name="noDifferences">The encoding of a non-symbolic TrueType font may not change names (/Differences; ISO 19005-1, 6.3.7): where
    /// it would, the font is written symbolic (with <paramref name="drawn"/>; without it not embedded).</param>
    public static Result? For(PdfDictionary font, PdfObject? encoding, PdfArray? widths, PdfDictionary? descriptor, bool[]? used,
        Func<int, (int Glyph, double Width, string? Text)>? drawn = null, bool noDifferences = false)
    {
        if (used is null) return null;
        // a font used only with empty text: it must be embedded all the same — with the space
        if (!used.Contains(true))
        {
            used = (bool[])used.Clone();
            used[32] = true;
        }
        if (font.GetName(PdfName.Subtype)?.Value is not ("Type1" or "MMType1" or "TrueType")) return null;
        var baseFont = font.GetName(PdfName.Get("BaseFont"))?.Value ?? "";
        if (!StandardFonts.IsStandard14(baseFont)) return null;
        var (file, symbol, dingbats) = StandardFonts.Choose(baseFont, descriptor);
        if (symbol || dingbats) return Symbolic(baseFont, file, dingbats, encoding, widths, (int)(font.GetInteger(PdfName.Get("FirstChar")) ?? 0), used);
        var flags = (int)(descriptor?.GetInteger(PdfName.Get("Flags")) ?? 0);
        // a symbolic font finds its glyphs in other ways (decided as in PdfFont; docs/FEHLERGEDAECHTNIS.md, No. 571)
        if (SimpleFont.SymbolicFlag(flags, font) || !file.EndsWith(".ttf", StringComparison.Ordinal)) return null;

        // the glyph name of each code, as the display reads it (without a base encoding: StandardEncoding for a Type 1 font, WinAnsiEncoding
        // for a TrueType font; docs/FEHLERGEDAECHTNIS.md, No. 769)
        var names = new string?[256];
        string[]? baseTable = null;
        switch (encoding)
        {
            case PdfName name:
                baseTable = PdfFont.EncodingTable(name.Value);
                break;
            case PdfDictionary dict:
                baseTable = PdfFont.EncodingTable(dict.GetName(PdfName.Get("BaseEncoding"))?.Value);
                if (dict.GetArray(PdfName.Get("Differences")) is { } differences)
                {
                    var code = 0;
                    for (var i = 0; i < differences.Count; i++)
                        switch (differences.Get(i))
                        {
                            case var number when number.AsNumber() is { } n:
                                code = (int)n;
                                break;
                            case PdfName glyph when code is >= 0 and < 256:
                                names[code++] = glyph.Value;
                                break;
                        }
                }
                break;
        }
        baseTable ??= font.GetName(PdfName.Subtype)?.Value == "TrueType" ? FontTables.WinAnsiEncoding : FontTables.StandardEncoding;
        for (var c = 0; c < 256; c++)
            names[c] ??= baseTable[c] is { Length: > 0 } n ? n : null;

        var program = EmbeddedFonts.StandardOpenType(file);
        var units = program.UnitsPerEm;
        var first = (int)(font.GetInteger(PdfName.Get("FirstChar")) ?? 0);
        // a code shown without a glyph (no name, .notdef, a name the font lacks): the display draws nothing for it; by name it would stand
        // for .notdef (6.2.11.8) — the font is written symbolic then, finding every glyph by its code
        if (Enumerable.Range(0, 256).Any(c => used[c] && (names[c] is not { } n || n == ".notdef" || !GlyphNames.InList(n) || GlyphNames.CodePoint(n) is var p && (p <= 0 ||
                program.GlyphForUnicode(p) <= 0))))
            return drawn is null ? null : SymbolicTrueType(file, program, used, drawn);
        var gids = new int[256];
        var own = new double[256];
        int low = 256, high = -1;
        for (var c = 0; c < 256; c++)
        {
            if (names[c] is not { } name) continue;
            var shown = used[c];
            // a code shown as .notdef draws nothing; written into /Differences, the name is no glyph name of the list PDF/A allows
            if (name == ".notdef")
            {
                if (shown) return null;
                continue;
            }
            // PDF/A: the names of a non-symbolic TrueType font must be in the Adobe Glyph List; a glyph the font lacks would show its .notdef
            var cp = GlyphNames.CodePoint(name);
            var gid = GlyphNames.InList(name) && cp > 0 ? program.GlyphForUnicode(cp) : 0;
            if (gid <= 0)
            {
                if (shown) return null;
                names[c] = null;
                continue;
            }
            gids[c] = gid;
            // the width the display sets the code with: the standard metrics for the 14 standard fonts, else the substitute's exact width
            own[c] = Shown(program.AdvanceWidth(gid) * 1000.0 / units, baseFont);
            if (shown && widths is not null && c - first is >= 0 and var at && at < widths.Count && widths.GetNumber(at) is { } given)
            {
                if (Math.Abs(given - own[c]) > 1) return null;   // the file sets the text with other widths: the page would change
                own[c] = given;   // within the tolerance of 6.2.11.5, and the text stays exactly where it was
            }
            low = Math.Min(low, c);
            high = Math.Max(high, c);
        }
        if (high < 0) return null;

        // encoding: WinAnsiEncoding, and /Differences where the names differ from it
        var differencesOut = new PdfArray();
        var last = -2;
        for (var c = low; c <= high; c++)
        {
            if (names[c] is not { } name || gids[c] == 0 || FontTables.WinAnsiEncoding[c] == name) continue;
            if (c != last + 1) differencesOut.Add(PdfInteger.Of(c));
            differencesOut.Add(PdfName.Get(name));
            last = c;
        }
        PdfObject encodingOut = PdfName.Get("WinAnsiEncoding");
        if (differencesOut.Count > 0 && noDifferences) return drawn is null ? null : SymbolicTrueType(file, program, used, drawn);
        if (differencesOut.Count > 0)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Get("Encoding"));
            dict.Set(PdfName.Get("BaseEncoding"), PdfName.Get("WinAnsiEncoding"));
            dict.Set(PdfName.Get("Differences"), differencesOut);
            encodingOut = dict;
        }
        var widthsOut = new PdfArray();
        for (var c = low; c <= high; c++) widthsOut.Add(gids[c] > 0 ? new PdfReal(own[c]) : PdfInteger.Of(0));

        var key = file + ":" + string.Join(',', gids);
        var tag = Tag(key);
        var fontName = PdfName.Get(tag + "+" + program.PostScriptName);
        var result = new PdfDictionary();
        result.Set(PdfName.Type, PdfName.Get("Font"));
        result.Set(PdfName.Subtype, PdfName.Get("TrueType"));
        result.Set(PdfName.Get("BaseFont"), fontName);
        result.Set(PdfName.Get("FirstChar"), PdfInteger.Of(low));
        result.Set(PdfName.Get("LastChar"), PdfInteger.Of(high));
        result.Set(PdfName.Get("Widths"), widthsOut);
        result.Set(PdfName.Get("Encoding"), encodingOut);

        var bytes = TrueTypeSubsetter.SubsetWithCmap(EmbeddedFonts.LoadStandard(file), gids.Where(g => g > 0).Append(0).Distinct());
        var programDict = new PdfDictionary();
        programDict.Set(PdfName.Get("Length1"), PdfInteger.Of(bytes.Length));
        return new Result(result, Descriptor(program, fontName, file), new PdfStream(programDict, bytes), key);
    }

    /// <summary>
    /// A standard font as a symbolic TrueType font (<see cref="For"/>): every shown code finds through a (3,0) cmap (F000 + code) the glyph the
    /// display draws for it — or an empty one where it draws nothing —, every glyph has the width the display sets the code with, and /ToUnicode
    /// (set by the writer) says what the display reads.
    /// </summary>
    private static Result SymbolicTrueType(string file, OpenTypeFont program, bool[] used, Func<int, (int Glyph, double Width, string? Text)> drawn)
    {
        var units = program.UnitsPerEm;
        var advances = new Dictionary<int, int>();
        var appended = new List<(int Glyph, int Advance)>();
        var cmap = new List<(int Code, int Glyph)>();
        var widthsOut = new PdfArray();
        int low = 256, high = -1;
        for (var c = 0; c < 256; c++)
            if (used[c])
            {
                low = Math.Min(low, c);
                high = Math.Max(high, c);
            }
        for (var c = low; c <= high; c++)
        {
            var (gid, width, _) = drawn(c);
            widthsOut.Add(new PdfReal(Math.Round(width, 9)));
            if (!used[c]) continue;
            var advance = (int)Math.Round(width * units / 1000);
            int glyph;
            if (gid > 0 && gid < program.GlyphCount && (!advances.TryGetValue(gid, out var known) || known == advance))
            {
                advances[gid] = advance;
                glyph = gid;
            }
            else
            {
                appended.Add((gid is >= 0 && gid < program.GlyphCount ? gid : -1, advance));
                glyph = program.GlyphCount + appended.Count - 1;
            }
            cmap.Add((0xF000 + c, glyph));
        }
        var bytes = TrueTypeSubsetter.Rebuild(EmbeddedFonts.LoadStandard(file), advances, appended, cmap);
        var key = file + ":symbolisch:" + string.Join(',', cmap.Select(m => $"{m.Code}={m.Glyph}")) + ":" + string.Join(',', widthsOut.Select(w => w.AsNumber()));
        var fontName = PdfName.Get(Tag(key) + "+" + program.PostScriptName);
        var result = new PdfDictionary();
        result.Set(PdfName.Type, PdfName.Get("Font"));
        result.Set(PdfName.Subtype, PdfName.Get("TrueType"));
        result.Set(PdfName.Get("BaseFont"), fontName);
        result.Set(PdfName.Get("FirstChar"), PdfInteger.Of(low));
        result.Set(PdfName.Get("LastChar"), PdfInteger.Of(high));
        result.Set(PdfName.Get("Widths"), widthsOut);
        var descriptor = Descriptor(program, fontName, file);
        descriptor.Set(PdfName.Get("Flags"), PdfInteger.Of(((int)(descriptor.GetInteger(PdfName.Get("Flags")) ?? 0) | 4) & ~32));
        var programDict = new PdfDictionary();
        programDict.Set(PdfName.Get("Length1"), PdfInteger.Of(bytes.Length));
        return new Result(result, descriptor, new PdfStream(programDict, bytes), key);
    }

    /// <summary>
    /// Symbol and ZapfDingbats, embedded as the bundled CFF fonts the display draws them with (Type 1 with a compact font program, FontFile3):
    /// the encoding names every code shown — the names the display looks the glyphs up by — so that no reader depends on the built-in one.
    /// </summary>
    /// <summary>
    /// The width the display sets a code of a font without /Widths with (1/1000 em): for the 14 standard fonts their standard metrics — the
    /// substitute's width rounded to whole units, as <c>SimpleFont</c> does (docs/FEHLERGEDAECHTNIS.md, No. 884) —, for the others the
    /// substitute's exact width. Within the tolerance of 6.2.11.5 either way (the rounding stays under a quarter of a unit). Before, the
    /// embedded font always took the exact width while the display set the standard metrics, and the text of a PDF/A copy crept along each line
    /// (No. 918).
    /// </summary>
    private static double Shown(double exact, string baseFont) => StandardFonts.HasStandardMetrics(baseFont) ? Math.Round(exact) : Math.Round(exact, 9);

    private static Result? Symbolic(string baseFont, string file, bool dingbats, PdfObject? encoding, PdfArray? widths, int first, bool[] used)
    {
        var names = new string?[256];
        string[]? baseTable = null;
        switch (encoding)
        {
            case PdfName name:
                baseTable = PdfFont.EncodingTable(name.Value);
                break;
            case PdfDictionary dict:
                baseTable = PdfFont.EncodingTable(dict.GetName(PdfName.Get("BaseEncoding"))?.Value);
                if (dict.GetArray(PdfName.Get("Differences")) is { } differences)
                {
                    var code = 0;
                    for (var i = 0; i < differences.Count; i++)
                        switch (differences.Get(i))
                        {
                            case var number when number.AsNumber() is { } n:
                                code = (int)n;
                                break;
                            case PdfName glyph when code is >= 0 and < 256:
                                names[code++] = glyph.Value;
                                break;
                        }
                }
                break;
        }
        baseTable ??= dingbats ? FontTables.ZapfDingbatsEncoding : FontTables.SymbolSetEncoding;
        var cff = EmbeddedFonts.StandardCff(file);
        var scale = cff.FontMatrix.A * 1000;
        var differencesOut = new PdfArray();
        var widthsOut = new PdfArray();
        int low = -1, high = -1, last = -2;
        var gids = new List<int>();
        var text = new Dictionary<int, string>();
        for (var c = 0; c < 256; c++)
        {
            if (!used[c]) continue;
            var name = names[c] ?? (baseTable[c] is { Length: > 0 } n ? n : null);
            // as the display: by name, otherwise through the font's own encoding — the written name is then the glyph's
            var gid = name is null ? -1 : cff.GlyphByName(name);
            if (gid <= 0 && cff.Encoding[c] > 0) gid = cff.Encoding[c];
            if (gid <= 0) return null;
            name = cff.GlyphName(gid) ?? name;
            if (name is null) return null;
            gids.Add(gid);
            if (low < 0) low = c;
            for (var gap = high + 1; high >= 0 && gap < c; gap++) widthsOut.Add(PdfInteger.Of(0));
            high = c;
            var width = Shown(cff.AdvanceWidth(gid) * scale, baseFont);
            if (widths is not null && c - first is >= 0 and var at && at < widths.Count && widths.GetNumber(at) is { } given)
            {
                if (Math.Abs(given - width) > 1) return null;
                width = given;
            }
            widthsOut.Add(new PdfReal(width));
            if (c != last + 1) differencesOut.Add(PdfInteger.Of(c));
            differencesOut.Add(PdfName.Get(name));
            last = c;
            // the names of ZapfDingbats (a1 …) are not in the Adobe Glyph List; the display reads them through their own list
            if (GlyphNames.ToUnicode(name, dingbats) is { Length: > 0 } reads) text[c] = reads;
        }
        if (low < 0) return null;
        var key = file + ":" + string.Join(',', gids);
        var fontName = PdfName.Get(baseFont);   // the name of the standard font: readers that know it treat it so
        var encodingOut = new PdfDictionary();
        encodingOut.Set(PdfName.Type, PdfName.Get("Encoding"));
        encodingOut.Set(PdfName.Get("Differences"), differencesOut);
        var result = new PdfDictionary();
        result.Set(PdfName.Type, PdfName.Get("Font"));
        result.Set(PdfName.Subtype, PdfName.Get("Type1"));
        result.Set(PdfName.Get("BaseFont"), fontName);
        result.Set(PdfName.Get("FirstChar"), PdfInteger.Of(low));
        result.Set(PdfName.Get("LastChar"), PdfInteger.Of(high));
        result.Set(PdfName.Get("Widths"), widthsOut);
        result.Set(PdfName.Get("Encoding"), encodingOut);
        var d = SymbolicDescriptor(fontName);
        var programDict = new PdfDictionary();
        programDict.Set(PdfName.Subtype, PdfName.Get("Type1C"));
        // the whole font program (small): the encoding names the glyphs
        return new Result(result, d, new PdfStream(programDict, EmbeddedFonts.LoadStandard(file)), key, "FontFile3",
            text.Count > 0 ? Editing.ElementFace.ToUnicode(text, codeBytes: 1) : null);
    }

    /// <summary>
    /// A standard font that is not embedded, with what PDF 2.0 asks of every simple font (ISO 32000-2, 9.6.2.1, Table 109 — the exception for
    /// the standard 14 fonts is deprecated there): /FirstChar, /LastChar and /Widths where it lacks them — the widths the display sets the codes
    /// with (the bundled font, metrically the same) — and a /FontDescriptor that names the standard font, without a font program (readers still
    /// take their own). See docs/FEHLERGEDAECHTNIS.md, No. 372.
    /// </summary>
    /// <param name="font">A font dictionary as read.</param>
    /// <param name="doc">The document it belongs to (its entries are resolved there).</param>
    /// <returns>A copy with the entries; null if it is no standard font without a descriptor.</returns>
    public static PdfDictionary? Described(PdfDictionary font, PdfDocument doc)
    {
        if (font.GetName(PdfName.Subtype)?.Value is not ("Type1" or "MMType1" or "TrueType") || font.ContainsKey(PdfName.Get("FontDescriptor")) ||
            font.GetName(PdfName.Type)?.Value is not (null or "Font") || font.GetName(PdfName.Get("BaseFont"))?.Value is not { } baseFont ||
            !StandardFonts.IsStandard14(baseFont))
            return null;
        var result = new PdfDictionary(font.Count + 4);
        foreach (var (key, value) in font.Entries) result.Set(key, value);
        if (font.Get(PdfName.Get("Widths")) is not PdfArray || font.GetInteger(PdfName.Get("FirstChar")) is null)
        {
            var shown = PdfFont.Get(font, doc) as SimpleFont;
            var drawn = shown?.DrawnTrueType(substitute: true);
            var widths = new double[256];
            int low = -1, high = -1;
            for (var c = 0; c < 256 && shown is not null; c++)
            {
                var glyph = shown.Glyph((uint)c, 1);
                // a code the display draws a glyph for (the space too); the others keep width 0
                var has = drawn is var (_, glyphOf) ? glyphOf(c) > 0 : !glyph.Outline.IsEmpty || glyph.Text == " ";
                if (!has) continue;
                // the widths the display sets the text with: for the standard 14 the standard metrics in whole thousandths, as other readers
                // set them too (FG 884); not rounded any further here (FG 372: glyphs would move by fractions of a pixel along a line)
                widths[c] = Math.Round(glyph.Width * 1000, 9);
                if (low < 0) low = c;
                high = c;
            }
            if (low >= 0)
            {
                var array = new PdfArray();
                for (var c = low; c <= high; c++) array.Add(new PdfReal(widths[c]));
                result.Set(PdfName.Get("FirstChar"), PdfInteger.Of(low));
                result.Set(PdfName.Get("LastChar"), PdfInteger.Of(high));
                result.Set(PdfName.Get("Widths"), array);
            }
        }
        var (file, symbol, dingbats) = StandardFonts.Choose(baseFont, null);
        var name = PdfName.Get(baseFont);
        result.Set(PdfName.Get("FontDescriptor"), symbol || dingbats || !file.EndsWith(".ttf", StringComparison.Ordinal)
            ? SymbolicDescriptor(name)
            : Descriptor(EmbeddedFonts.StandardOpenType(file), name, file));
        return result;
    }

    /// <summary>The font descriptor of Symbol and ZapfDingbats (symbolic: the encoding of the font program names the glyphs).</summary>
    /// <summary>
    /// A font descriptor for a simple font that is not embedded, from the bundled substitute the display draws it with (symbolic for Symbol and
    /// ZapfDingbats), without a font program — readers still take their own. For a font whose own /FontDescriptor is no font descriptor at all
    /// (docs/FEHLERGEDAECHTNIS.md, No. 886).
    /// </summary>
    /// <param name="font">The font dictionary as read.</param>
    /// <returns>A new descriptor naming the font's /BaseFont.</returns>
    internal static PdfDictionary SubstituteDescriptor(PdfDictionary font)
    {
        var baseFont = font.GetName(PdfName.Get("BaseFont"))?.Value ?? "Helvetica";
        var (file, symbol, dingbats) = StandardFonts.Choose(baseFont, null);
        var name = PdfName.Get(baseFont);
        return symbol || dingbats || !file.EndsWith(".ttf", StringComparison.Ordinal) ? SymbolicDescriptor(name)
            : Descriptor(EmbeddedFonts.StandardOpenType(file), name, file);
    }

    private static PdfDictionary SymbolicDescriptor(PdfName fontName)
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Type, PdfName.Get("FontDescriptor"));
        d.Set(PdfName.Get("FontName"), fontName);
        d.Set(PdfName.Get("Flags"), PdfInteger.Of(4));
        d.Set(PdfName.Get("FontBBox"), new PdfArray([PdfInteger.Of(-1), PdfInteger.Of(-293), PdfInteger.Of(1050), PdfInteger.Of(1010)]));
        d.Set(PdfName.Get("ItalicAngle"), PdfInteger.Of(0));
        d.Set(PdfName.Get("Ascent"), PdfInteger.Of(1010));
        d.Set(PdfName.Get("Descent"), PdfInteger.Of(-293));
        d.Set(PdfName.Get("CapHeight"), PdfInteger.Of(700));
        d.Set(PdfName.Get("StemV"), PdfInteger.Of(90));
        return d;
    }

    /// <summary>The font descriptor from the font's own tables (head, hhea, post, OS/2), in thousandths of the em.</summary>
    private static PdfDictionary Descriptor(OpenTypeFont font, PdfName name, string file)
    {
        var scale = 1000.0 / font.UnitsPerEm;
        PdfInteger Scaled(double v) => PdfInteger.Of((long)Math.Round(v * scale));
        var head = font.TableData("head");
        var box = head.Length >= 44
            ? new PdfArray([Scaled(BinaryPrimitives.ReadInt16BigEndian(head[36..])), Scaled(BinaryPrimitives.ReadInt16BigEndian(head[38..])),
                Scaled(BinaryPrimitives.ReadInt16BigEndian(head[40..])), Scaled(BinaryPrimitives.ReadInt16BigEndian(head[42..]))])
            : new PdfArray([PdfInteger.Of(-200), PdfInteger.Of(-300), PdfInteger.Of(1200), PdfInteger.Of(1000)]);
        var post = font.TableData("post");
        var italicAngle = post.Length >= 8 ? BinaryPrimitives.ReadInt32BigEndian(post[4..]) / 65536.0 : 0;
        var os2 = font.TableData("OS/2");
        var capHeight = os2.Length >= 90 && BinaryPrimitives.ReadUInt16BigEndian(os2) >= 2 ? BinaryPrimitives.ReadInt16BigEndian(os2[88..]) : font.Ascent;
        var bold = file.Contains("Bold", StringComparison.Ordinal);
        var flags = 32 | (file.StartsWith("LiberationMono", StringComparison.Ordinal) ? 1 : 0) | (file.StartsWith("LiberationSerif", StringComparison.Ordinal) ? 2 : 0) |
                    (italicAngle != 0 ? 64 : 0);
        var d = new PdfDictionary();
        d.Set(PdfName.Type, PdfName.Get("FontDescriptor"));
        d.Set(PdfName.Get("FontName"), name);
        d.Set(PdfName.Get("Flags"), PdfInteger.Of(flags));
        d.Set(PdfName.Get("FontBBox"), box);
        d.Set(PdfName.Get("ItalicAngle"), new PdfReal(Math.Round(italicAngle, 2)));
        d.Set(PdfName.Get("Ascent"), Scaled(font.Ascent));
        d.Set(PdfName.Get("Descent"), Scaled(font.Descent));
        d.Set(PdfName.Get("CapHeight"), Scaled(capHeight));
        d.Set(PdfName.Get("StemV"), PdfInteger.Of(bold ? 140 : 80));
        return d;
    }

    /// <summary>The six capital letters before the name of a subset (ISO 32000-2, 9.9.2), from what the subset holds.</summary>
    private static string Tag(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var sb = new StringBuilder(6);
        for (var i = 0; i < 6; i++) sb.Append((char)('A' + hash[i] % 26));
        return sb.ToString();
    }
}
