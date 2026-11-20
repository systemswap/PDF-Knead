using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Fonts;

/// <summary>A code in the embedded font program as ISO 32000-1 finds it (<see cref="PdfFont.Strict"/>).</summary>
/// <param name="Embedded">The font has an embedded program that could be read.</param>
/// <param name="Present">The program has a glyph for the code other than .notdef.</param>
/// <param name="Notdef">The code stands for the .notdef glyph: by the name the encoding gives it (or none) in simple fonts, by a missing glyph
/// in composite ones.</param>
/// <param name="ProgramWidth">The advance of the glyph in the program, in 1/1000 em; null if no glyph was found.</param>
/// <param name="DictionaryWidth">The width the font dictionary gives the code, in 1/1000 em.</param>
/// <param name="Name">The name of the glyph in a Type 1 or CFF program of a simple font (what its /CharSet lists); null otherwise.</param>
internal readonly record struct StrictGlyph(bool Embedded, bool Present, bool Notdef, double? ProgramWidth, double DictionaryWidth, string? Name = null);

internal sealed partial class SimpleFont
{
    /// <summary>The text of a code as ISO 32000-2, 9.10.2 derives it (what PDF/A level u asks for, 6.2.11.7.2): /ToUnicode, else the glyph
    /// name of the encoding (or of the built-in encoding of a Type 1 or CFF program) in the Adobe Glyph List or of the uniXXXX form — not the code
    /// itself, nor other names from the font program.</summary>
    internal override string? StrictText(uint code, int length) =>
        _toUnicode?.ToUnicode(code, 1) ?? ((_names[code & 0xFF] ?? BuiltInName((int)(code & 0xFF))) is { } name ? GlyphNames.ToUnicode(name, _dingbats) : null);

    internal override bool ForbiddenUnicode(uint code, int length) => _toUnicode?.ForbiddenTarget(code, 1) == true;

    /// <summary>The glyph name the built-in encoding of an embedded Type 1 or CFF program gives a code; null if none.</summary>
    private string? BuiltInName(int code) =>
        !_program.IsEmbedded ? null
        : _program.Type1 is { } t1 ? t1.Encoding[code] is { Length: > 0 } name ? name : null
        : _program.Cff is { IsCidKeyed: false } cff && !_program.IsTrueType && cff.Encoding[code] > 0 ? cff.GlyphName(cff.Encoding[code]) : null;

    /// <summary>
    /// The font has an embedded program the display reads and a strict reader would read too (PDF/A: rebuilding a font from its program;
    /// otherwise it is embedded as the substitute from what the display draws). A truncated TrueType program is not (<see cref="OpenTypeFont.Intact"/>).
    /// </summary>
    internal bool HasEmbeddedProgram => _program.IsEmbedded && _program.OpenType is not { Intact: false };

    /// <summary>The font matrix of the program: glyph space → text space.</summary>
    internal Graphics.Matrix ProgramMatrix => _program.FontMatrix;

    internal override StrictGlyph? Strict(uint code, int length)
    {
        var c = (int)(code & 0xFF);
        var dictionaryWidth = (_hasWidth[c] ? _widths[c] : _missingWidth) * 1000;
        if (!HasEmbeddedProgram) return new StrictGlyph(false, false, false, null, dictionaryWidth);
        var scale = _program.FontMatrix.A * 1000;
        StrictGlyph Found(bool present, bool notdef, double width, string? name = null) =>
            new(true, present, notdef, present ? width * scale : null, dictionaryWidth, name);
        if (_program.Type1 is { } t1)
        {
            // the name from the encoding of the dictionary; without a base encoding there, the font's built-in one
            var name = _names[c] ?? (!_hasBaseEncoding && t1.Encoding[c] is { Length: > 0 } builtin ? builtin : ".notdef");
            var present = name != ".notdef" && t1.HasGlyph(name);
            return Found(present, name == ".notdef", present ? t1.AdvanceWidth(name) : 0, name);
        }
        if (_program.Cff is { } cff && !_program.IsTrueType)
        {
            var name = _names[c] ?? (_hasBaseEncoding || cff.IsCidKeyed ? null : cff.GlyphName(cff.Encoding[c]));
            var gid = cff.IsCidKeyed ? cff.GlyphByCid(c)
                : _names[c] is { } named ? named == ".notdef" ? 0 : cff.GlyphByName(named)
                : _hasBaseEncoding ? 0 : cff.Encoding[c];
            var present = gid > 0 && gid < cff.GlyphCount;
            return Found(present, !cff.IsCidKeyed && (name is null or ".notdef"), present ? cff.AdvanceWidth(gid) : 0,
                present && !cff.IsCidKeyed ? cff.GlyphName(gid) : null);
        }
        if (_program.OpenType is { } ot)
        {
            var gid = StrictTrueType(ot, c);
            var present = gid > 0 && gid < ot.GlyphCount;
            // a symbolic font reaches its glyphs by code, not by name
            var notdef = _names[c] == ".notdef" || (_flags & 4) == 0 && _names[c] is null;
            return Found(present, notdef, present ? ot.AdvanceWidth(gid) : 0);
        }
        return new StrictGlyph(false, false, false, null, dictionaryWidth);
    }

    /// <summary>
    /// The glyph of a code in a TrueType program (ISO 32000-1, 9.6.6.4): non-symbolic fonts through the glyph name of the encoding — its
    /// Unicode value in the (3,1) cmap (or a cmap of the Unicode platform), its Mac Roman code in the (1,0) cmap, the name in the program's own
    /// list (post) or a name "g123" for the glyph index, in this order, as strict validators accept it; symbolic fonts through the code in the
    /// (3,0) cmap (also in the ranges F000, F100, F200), otherwise in the (1,0) cmap — no other. 0 if nothing is found.
    /// </summary>
    private int StrictTrueType(OpenTypeFont ot, int code)
    {
        if ((_flags & 4) == 0)
        {
            if (_names[code] is not { } name || name == ".notdef") return 0;
            if (GlyphNames.CodePoint(name) is > 0 and var cp && UnicodeGlyph(ot, cp) is > 0 and var unicode) return unicode;
            if (Array.IndexOf(FontTables.MacRomanEncoding, name) is >= 0 and var mac && ot.Lookup(1, 0, mac) is > 0 and var roman) return roman;
            if (ot.GlyphByName(name) is > 0 and var byName) return byName;
            return GlyphIndexName(name, ot.GlyphCount);
        }
        if (ot.HasCmap(3, 0))
        {
            foreach (var prefix in (ReadOnlySpan<int>)[0, 0xF000, 0xF100, 0xF200])
                if (ot.Lookup(3, 0, prefix + code) is > 0 and var g) return g;
            return 0;
        }
        return ot.HasCmap(1, 0) ? Math.Max(0, ot.Lookup(1, 0, code)) : 0;
    }
}

internal sealed partial class SimpleFont
{
    /// <summary>The glyph index a name "g123" stands for (producers of such names mean it so); 0 for other names and indices outside the font.</summary>
    internal static int GlyphIndexName(string name, int glyphs) =>
        name.Length > 1 && name[0] == 'g' && int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.None, null, out var index) && index < glyphs ? index : 0;

    /// <summary>A character in the (3,1) cmap or one of the Unicode platform; 0 if none has it.</summary>
    internal static int UnicodeGlyph(OpenTypeFont ot, int codePoint)
    {
        if (ot.Lookup(3, 1, codePoint) is > 0 and var g) return g;
        foreach (var (platform, encoding) in ot.CmapSubtables)
            if (platform == 0 && ot.Lookup(0, encoding, codePoint) is > 0 and var u) return u;
        return 0;
    }
}

internal sealed partial class CompositeFont
{
    /// <summary>The text of a code as ISO 32000-2, 9.10.2 derives it: /ToUnicode, else the character of the CID in one of the four Adobe
    /// CJK collections — not the inverted cmap of the font program.</summary>
    internal override string? StrictText(uint code, int length) =>
        _toUnicode?.ToUnicode(code, length) ?? PredefinedCMaps.Text(_ordering, _cmap.ToCid(code, length));

    internal override bool ForbiddenUnicode(uint code, int length) => _toUnicode?.ForbiddenTarget(code, length) == true;

    /// <summary>The font has an embedded program the display reads and a strict reader would read too (see the simple font's).</summary>
    internal bool HasEmbeddedProgram => _program is { IsEmbedded: true } && _program.OpenType is not { Intact: false };

    /// <summary>The font matrix of the program the display draws with (glyph space → text space).</summary>
    internal Graphics.Matrix ProgramMatrix => _program?.FontMatrix ?? new Graphics.Matrix(0.001, 0, 0, 0.001, 0, 0);

    internal override StrictGlyph? Strict(uint code, int length)
    {
        var cid = _cmap.ToCid(code, length);
        var dictionaryWidth = Width(cid) * 1000;
        if (!HasEmbeddedProgram || _program is not { } program) return new StrictGlyph(false, false, false, null, dictionaryWidth);
        var scale = program.FontMatrix.A * 1000;
        // a program of the other kind (CFF under CIDFontType2, TrueType under CIDFontType0): its glyphs are not judged
        if (program.IsTrueType != _cidFontType2) return null;
        if (program.IsTrueType && program.OpenType is { } ot)
        {
            // CIDFontType2: through the CIDToGIDMap, identity without one (ISO 32000-1, 9.7.4.2); a glyph index inside the font counts, as
            // strict validators take it (glyph 0 included)
            var gid = _cidToGid is { } map ? 2 * cid + 1 < map.Length ? map[2 * cid] << 8 | map[2 * cid + 1] : 0 : cid;
            var present = gid < ot.GlyphCount;
            // CID 0 is the .notdef of every character collection
            return new StrictGlyph(true, present, cid == 0, present ? ot.AdvanceWidth(gid) * scale : null, dictionaryWidth);
        }
        if (program.Cff is { } cff)
        {
            // CIDFontType0: the CID through the charset of a CID-keyed font, otherwise the CID is the glyph index
            var gid = cff.IsCidKeyed ? cff.GlyphByCid(cid) : cid;
            var present = gid > 0 && gid < cff.GlyphCount;
            return new StrictGlyph(true, present, !present, present ? cff.AdvanceWidth(gid) * scale : null, dictionaryWidth);
        }
        // a Type 1 program cannot be the program of a CIDFont
        return new StrictGlyph(false, false, false, null, dictionaryWidth);
    }
}

internal sealed partial class Type3Font
{
    /// <summary>The text of a code as ISO 32000-2, 9.10.2 derives it: /ToUnicode, else the glyph name of the encoding.</summary>
    internal override string? StrictText(uint code, int length) =>
        _toUnicode?.ToUnicode(code, 1) ?? (_names[code & 0xFF] is { } name ? GlyphNames.ToUnicode(name) : null);

    internal override bool ForbiddenUnicode(uint code, int length) => _toUnicode?.ForbiddenTarget(code, 1) == true;

    /// <summary>
    /// A Type 3 font has a glyph for a code when its encoding names a procedure of the font (ISO 32000-1, 9.6.5); the width of the glyph is
    /// the first operand of its d0 or d1, in glyph space like /Widths — 0 for a procedure that sets none or cannot be read, as strict readers
    /// take it (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    internal override StrictGlyph? Strict(uint code, int length)
    {
        var c = (int)(code & 0xFF);
        var name = _names[c] ?? _baseNames?[c];
        var proc = name is { Length: > 0 } ? _charProcs?.Get(PdfName.Get(name)) as PdfStream : null;
        return new StrictGlyph(true, proc is not null, name == ".notdef", proc is null ? null : ProcWidth(proc) ?? 0, _glyphSpaceWidths[c]);
    }

    /// <summary>The glyph procedure the display draws for a code; null if there is none.</summary>
    internal PdfStream? ProcOf(uint code)
    {
        var c = (int)(code & 0xFF);
        return (_names[c] ?? _baseNames?[c]) is { Length: > 0 } name ? _charProcs?.Get(PdfName.Get(name)) as PdfStream : null;
    }

    /// <summary>A d0 with two numbers or a d1 with six (ISO 32000-1, Table 113).</summary>
    internal static bool WellFormed(string op, IReadOnlyList<Content.Operand> operands) =>
        (op, operands.Count) is ("d0", 2) or ("d1", 6) && operands.All(o => o.IsNumber);

    /// <summary>The width a glyph procedure sets with d0 or d1 (its first operator); null if it sets none or cannot be read.</summary>
    internal double? ProcWidth(PdfStream proc)
    {
        try
        {
            var parser = new Content.ContentParser(_doc.GetStreamData(proc));
            // exactly its operands, all numbers (ISO 32000-1, Table 113): strict readers take no width from anything else
            return parser.Next() is { } op && WellFormed(op, parser.Operands) ? parser.Operands[0].Number : null;
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return null;
        }
    }
}
