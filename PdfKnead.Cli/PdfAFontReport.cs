using System.Globalization;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Cli;

/// <summary>
/// pdf-knead pdfa-schriften &lt;file&gt;: the fonts of the pages as PDF/A looks at them (docs/PRUEFUNGEN.md, section 6p) — kind, form of the
/// program, cmap tables, encoding, and per code with a width the glyph the strict lookup finds, with both widths. For finding why a check
/// of the glyphs differs from a reference validator.
/// </summary>
internal static class PdfAFontReport
{
    public static int Run(string file)
    {
        using var doc = PdfDocument.Open(file);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var p = 0; p < doc.PageCount; p++)
        {
            if (doc.Pages[p].Resources?.GetDictionary(PdfName.Get("Font")) is not { } fonts) continue;
            foreach (var (key, _) in fonts.Entries)
                if (fonts.GetDictionary(key) is { } font && seen.Add(font)) Report(doc, p + 1, key.Value, font);
        }
        return 0;
    }

    private static void Report(PdfDocument doc, int page, string key, PdfDictionary dict)
    {
        var kind = dict.GetName(PdfName.Subtype)?.Value;
        var described = kind == "Type0" && dict.Get(PdfName.Get("DescendantFonts")) is PdfArray { Count: > 0 } d && d.Get(0) is PdfDictionary cid ? cid : dict;
        var descriptor = described.GetDictionary(PdfName.Get("FontDescriptor"));
        Console.WriteLine($"S{page} /{key}  {kind}/{described.GetName(PdfName.Subtype)?.Value}  {dict.GetName(PdfName.Get("BaseFont"))?.Value}  Flags {descriptor?.GetInteger(PdfName.Get("Flags"))}");
        Console.WriteLine($"   Encoding: {Short(dict.Get(PdfName.Get("Encoding")))}");
        Console.WriteLine($"   FirstChar {Short(dict.Get(PdfName.Get("FirstChar")))}, LastChar {Short(dict.Get(PdfName.Get("LastChar")))}, " +
                          $"Widths {Short(dict.Get(PdfName.Get("Widths")))}" + (kind == "Type3" ? $", FontMatrix {Short(dict.Get(PdfName.Get("FontMatrix")))}" : ""));
        if (descriptor is not null)
            foreach (var file in (string[])["FontFile", "FontFile2", "FontFile3"])
                if (descriptor.GetStream(PdfName.Get(file)) is { } stream)
                {
                    byte[] head;
                    try { head = doc.DecodeStream(stream, 16).Data; } catch (PdfException) { head = []; }
                    Console.WriteLine($"   {file} {stream.Dictionary.GetName(PdfName.Subtype)?.Value}  {Convert.ToHexString(head.AsSpan(0, Math.Min(8, head.Length)))}");
                    if (FontProgram.Embedded(descriptor, doc) is { } program)
                        Console.WriteLine($"   gelesen: {(program.Type1 is not null ? "Type 1" : program.IsTrueType ? "TrueType" : "CFF")}" +
                                          (program.OpenType is { } ot ? $", cmap {string.Join(" ", ot.CmapSubtables.Select(c => $"({c.Platform},{c.Encoding})"))}, {ot.GlyphCount} Glyphen, " +
                                                                          $"„A“: {string.Join(" ", ot.CmapSubtables.Select(c => ot.Lookup(c.Platform, c.Encoding, c.Platform == 1 ? 65 : 0x41)))}, Name A {ot.GlyphByName("A")}" : ""));
                    else Console.WriteLine("   nicht lesbar");
                }
        var font = PdfFont.Get(dict, doc);
        IEnumerable<(uint, int)> codes = kind == "Type0" ? Enumerable.Range(0, 65536).Select(c => ((uint)c, 2)) : Enumerable.Range(0, 256).Select(c => ((uint)c, 1));
        var shown = 0;
        foreach (var (code, length) in codes)
        {
            if (font.Strict(code, length) is not { } g) break;
            if (kind != "Type0" && g.DictionaryWidth == 0) continue;
            if (kind == "Type0" && !g.Present) continue;
            var bad = !g.Present || g.ProgramWidth is { } w && Math.Abs(w - g.DictionaryWidth) > 1;
            if (!bad || ++shown > 40) continue;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"   Code {code,5}: {(g.Embedded ? g.Present ? "da" : "FEHLT" : "nicht eingebettet")}  Breite PDF {g.DictionaryWidth:0.#}  Programm {g.ProgramWidth:0.#}"));
        }
    }

    private static string Short(PdfObject value)
    {
        var text = value.ToString() ?? "";
        text = string.Join(' ', text.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()));
        return text.Length > 200 ? text[..200] + " …" : text;
    }
}
