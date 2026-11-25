using System.Collections.Concurrent;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Fonts;

/// <summary>
/// A glyph of a PDF font, ready to draw: outline in font units, mapping into text space
/// (1 = font size), advance and text.
/// </summary>
/// <param name="Outline">Outline in font units; empty for Type 3 glyphs and glyphs without an outline.</param>
/// <param name="ToText">Mapping from font units into text space (font matrix, possibly stretched to the PDF width).</param>
/// <param name="Width">Horizontal advance w0 in text space.</param>
/// <param name="Text">Unicode text of the glyph; null if unknown.</param>
/// <param name="Procedure">Type 3: content stream of the glyph instead of an outline.</param>
public sealed record PdfGlyph(GlyphOutline Outline, Matrix ToText, double Width, string? Text, PdfStream? Procedure = null)
{
    /// <summary>Vertical writing: advance w1y (usually negative) in text space.</summary>
    public double VerticalAdvance { get; init; }

    /// <summary>Vertical writing: x of the position vector v of the glyph relative to the horizontal origin, in text space.</summary>
    public double OriginX { get; init; }

    /// <summary>Vertical writing: y of the position vector v of the glyph relative to the horizontal origin, in text space.</summary>
    public double OriginY { get; init; }
}

/// <summary>
/// Font of a PDF file (ISO 32000-2, 9.5–9.10): reads character codes from text strings and
/// returns the matching glyphs. Each font is built once per document and then used by all pages,
/// also concurrently.
/// </summary>
public abstract class PdfFont
{
    // glyph cache with a budget of 16 MB (96 bytes counted per entry; the outlines themselves are held by the font program):
    // codes of up to 4 bytes would otherwise make it unbounded
    private readonly BoundedCache<(uint Code, int Length), PdfGlyph> _glyphs = new(16L << 20, _ => 96);

    /// <summary>Creates a font with the given /BaseFont name.</summary>
    /// <param name="baseFont">The /BaseFont name; empty if the dictionary has none.</param>
    protected PdfFont(string baseFont) => BaseFont = baseFont;

    /// <summary>The /BaseFont name, including any subset prefix ("ABCDEF+Name").</summary>
    public string BaseFont { get; }

    /// <summary>The font dictionary the font was built from (text editing: style attributes, the same font for new text).</summary>
    public PdfDictionary? Dictionary { get; private set; }

    /// <summary>Object number of the font dictionary in its file, if it is an indirect object (new text refers to it when saved).</summary>
    public PdfObjectId? Id { get; internal set; }

    private Dictionary<int, (uint Code, int Length)>? _codes;

    /// <summary>
    /// Code for each character (Unicode code point) the font can show — the inverse of ToUnicode or the encoding (text editing:
    /// new text in the file's own font if it has all characters; see KONZEPT.md, section 5.14). Only single characters, no ligatures;
    /// characters without an outline are missing (not contained in a subset), whitespace counts. Type 3: a character counts if its
    /// procedure paints something (<see cref="ShowsInk"/>).
    /// </summary>
    /// <returns>The map, computed on the first call and then cached; at most 70,000 candidate codes are examined.</returns>
    public IReadOnlyDictionary<int, (uint Code, int Length)> CodesByUnicode()
    {
        if (_codes is { } known) return known;
        var map = new Dictionary<int, (uint Code, int Length)>();
        var tried = 0;
        var bytes = new byte[4];
        foreach (var (code, length) in CandidateCodes())
        {
            if (++tried > 70_000) break;
            // code and length as the font's encoding reads them: ToUnicode can give a different length than the CMap (Identity-H with a
            // 1-byte ToUnicode), and writing such codes would produce garbled text
            if (length is < 1 or > 4) continue;
            for (var b = 0; b < length; b++) bytes[b] = (byte)(code >> (8 * (length - 1 - b)));
            if (ReadCode(bytes.AsSpan(0, length), 0, out var read) != length || read != code) continue;
            var glyph = Glyph(code, length);
            if (glyph.Text is not { Length: > 0 } text) continue;
            if (Rune.DecodeFromUtf16(text, out var rune, out var used) != System.Buffers.OperationStatus.Done || used != text.Length) continue;
            if (!Rune.IsWhiteSpace(rune) && !ShowsInk(glyph, code)) continue;
            // a space without width is none (a Type 3 font without a space glyph gets the text of code 32 by default, width 0): new text set
            // with it ran its words together (No. 611)
            if (Rune.IsWhiteSpace(rune) && !(glyph.Width > 0)) continue;
            map.TryAdd(rune.Value, (code, length));
        }
        return _codes = map;
    }

    /// <summary>Whether a glyph draws something (text editing: a glyph that draws nothing is missing from a subset).</summary>
    /// <param name="glyph">The glyph.</param>
    /// <param name="code">Its code.</param>
    /// <returns>True if it has an outline.</returns>
    protected virtual bool ShowsInk(PdfGlyph glyph, uint code) => glyph.Procedure is null && !glyph.Outline.IsEmpty;

    /// <summary>Codes under which the font may have characters (simple fonts: the 256 one-byte codes; composite fonts: those from ToUnicode).</summary>
    /// <returns>Code and length in bytes of each candidate.</returns>
    protected virtual IEnumerable<(uint Code, int Length)> CandidateCodes()
    {
        for (uint c = 0; c < 256; c++) yield return (c, 1);
    }

    /// <summary>Vertical writing mode (WMode 1).</summary>
    public bool Vertical { get; protected init; }

    /// <summary>Ascent in text space (1 = font size), for highlighting and text selection; 0.8 unless the font gives a plausible value.</summary>
    public double Ascent { get; protected set; } = 0.8;

    /// <summary>Descent in text space (negative), for highlighting and text selection; -0.2 unless the font gives a plausible value.</summary>
    public double Descent { get; protected set; } = -0.2;

    /// <summary>Type 3: resources of the glyph procedures; null for other fonts or if the font has none.</summary>
    public virtual PdfDictionary? Type3Resources => null;

    /// <summary>True for Type 3 fonts.</summary>
    public virtual bool IsType3 => false;

    /// <summary>Reads the next code from a text string.</summary>
    /// <param name="s">The string bytes.</param>
    /// <param name="pos">Start position; must be inside <paramref name="s"/>.</param>
    /// <param name="code">The code read.</param>
    /// <returns>The number of bytes consumed (at least 1).</returns>
    public virtual int ReadCode(ReadOnlySpan<byte> s, int pos, out uint code)
    {
        code = s[pos];
        return 1;
    }

    /// <summary>Returns the glyph for a code. Thread-safe; glyphs are cached per font within a memory budget.</summary>
    /// <param name="code">The code, as returned by <see cref="ReadCode"/>.</param>
    /// <param name="length">Length of the code in bytes.</param>
    /// <returns>The glyph; for a broken glyph an empty outline with width 0.5.</returns>
    public PdfGlyph Glyph(uint code, int length) =>
        // look it up first, so that the lambda (which captures this) is only created the first time per code
        _glyphs.TryGetValue((code, length), out var glyph) ? glyph : _glyphs.GetOrAdd((code, length), k =>
    {
        try
        {
            return Load(k.Code, k.Length);
        }
        catch (Exception e) when (e is FontFormatException or PdfException or IndexOutOfRangeException or ArgumentException)
        {
            return new PdfGlyph(GlyphOutline.Empty, Matrix.Scale(0.001, 0.001), 0.5, null);
        }
    });

    /// <summary>Builds the glyph for a code; called once per code by <see cref="Glyph"/>.</summary>
    /// <param name="code">The code.</param>
    /// <param name="length">Length of the code in bytes.</param>
    /// <returns>The glyph.</returns>
    protected abstract PdfGlyph Load(uint code, int length);

    /// <summary>
    /// Compression and redaction: all glyphs of an embedded TrueType font through which a reader could find this code — a superset, because
    /// different readers choose different cmap subtables, always with the glyph the display draws. Empty if the font is not an embedded
    /// TrueType font.
    /// </summary>
    internal virtual IReadOnlyList<int> TrueTypeGlyphs(uint code, int length) => [];

    /// <summary>
    /// Redaction (<see cref="Writing.FontScrub"/>): the glyphs of the embedded font program that this code may show, by every way a
    /// reader may look it up — glyph indices (TrueType, CFF) and glyph names (Type 1). What no code still shown reaches can leave the
    /// font. Empty if the font is not embedded.
    /// </summary>
    internal virtual (IReadOnlyList<int> Gids, IReadOnlyList<string> Names) ProgramGlyphs(uint code, int length) => ([], []);

    /// <summary>
    /// PDF/A (ISO 19005-2, 6.2.11): a code against the embedded font program, looked up as ISO 32000-1 prescribes (9.6.6, 9.7.4) — without
    /// the fallbacks of drawing, which other readers do not share.
    /// </summary>
    /// <param name="code">The code.</param>
    /// <param name="length">Length of the code in bytes.</param>
    /// <returns>What the program has for the code; null for Type 3 fonts.</returns>
    internal virtual StrictGlyph? Strict(uint code, int length) => null;

    /// <summary>The text of a code as ISO 32000-2, 9.10.2 derives it (PDF/A level u); for other fonts the text the display reads.</summary>
    internal virtual string? StrictText(uint code, int length) => Glyph(code, length).Text;

    /// <summary>Whether /ToUnicode maps the code to the value 0, U+FEFF or U+FFFE (which the text leaves out; docs/FEHLERGEDAECHTNIS.md, No. 501).</summary>
    internal virtual bool ForbiddenUnicode(uint code, int length) => false;

    /// <summary>The CID of a code (composite fonts; -1 for simple fonts).</summary>
    internal virtual int Cid(uint code, int length) => -1;

    /// <summary>
    /// For a composite font: the horizontal width of a CID in thousandths of text space, exactly as this display reads it from /W and /DW
    /// (also where /W names a CID twice); NaN for other fonts.
    /// </summary>
    internal virtual double CidWidth(int cid) => double.NaN;

    /// <summary>
    /// For a simple font with a TrueType or OpenType program: the glyph the display draws for a code if <paramref name="program"/> were its
    /// program (-1 for none, also for a glyph index beyond the program); <see cref="int.MinValue"/> for other fonts. Used to check that a
    /// rewritten program draws every shown code as before.
    /// </summary>
    internal virtual int DrawnGlyphWith(OpenTypeFont program, uint code, int length) => int.MinValue;

    /// <summary>Whether word spacing applies: only for the single-byte code 32 (ISO 32000-2, 9.3.3).</summary>
    /// <param name="code">The code.</param>
    /// <param name="length">Length of the code in bytes.</param>
    /// <returns>True for the single-byte code 32.</returns>
    public static bool IsWordSpace(uint code, int length) => length == 1 && code == 32;

    // ------------------------------------------------------------ Creation

    /// <summary>Returns the font for a font dictionary, built once per document. Thread-safe.</summary>
    /// <param name="dict">The font dictionary.</param>
    /// <param name="doc">The document that owns the dictionary and caches the font.</param>
    /// <returns>The font; an unreadable font yields a fallback that keeps the text visible.</returns>
    public static PdfFont Get(PdfDictionary dict, PdfDocument doc) =>
        doc.Built.TryGetValue(dict, out var font) ? (PdfFont)font : (PdfFont)doc.Built.GetOrAdd(dict, _ => Create(dict, doc));

    /// <summary>A font for a dictionary built anew and not kept by the document: to read a changed copy of a font as the display would.</summary>
    internal static PdfFont Uncached(PdfDictionary dict, PdfDocument doc) => Create(dict, doc);

    private static PdfFont Create(PdfDictionary dict, PdfDocument doc)
    {
        PdfFont font;
        try
        {
            font = dict.GetName(PdfName.Subtype)?.Value switch
            {
                "Type0" => new CompositeFont(dict, doc),
                "Type3" => new Type3Font(dict, doc),
                _ => new SimpleFont(dict, doc),
            };
        }
        catch (Exception e) when (e is FontFormatException or PdfException or IndexOutOfRangeException or ArgumentException or InvalidCastException)
        {
            font = SimpleFont.Fallback(dict);
        }
        font.Dictionary = dict;
        return font;
    }

    internal static CMap? ReadToUnicode(PdfDictionary dict, PdfDocument doc)
    {
        if (dict.GetStream(PdfName.Get("ToUnicode")) is not { } stream) return null;
        try
        {
            // a ToUnicode CMap may be based on another one through /UseCMap (ISO 32000-2, 9.10.3; docs/FEHLERGEDAECHTNIS.md, No. 772)
            var cmap = CompositeFont.ParseUsing(stream, doc, 0);
            return cmap.HasUnicode ? cmap : null;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <summary>Reads ascent and descent from the FontDescriptor (in 1/1000), keeping the defaults for implausible values.</summary>
    /// <param name="descriptor">The FontDescriptor; may be null.</param>
    protected void ReadMetrics(PdfDictionary? descriptor)
    {
        var ascent = descriptor?.GetNumber(PdfName.Get("Ascent")) ?? 0;
        var descent = descriptor?.GetNumber(PdfName.Get("Descent")) ?? 0;
        if (ascent is > 100 and < 2000) Ascent = ascent / 1000;
        if (descent is < -10 and > -1000) Descent = descent / 1000;
    }

    /// <summary>Glyph name table of a named encoding (ISO 32000-2, 9.6.5); null for unknown names.</summary>
    internal static string[]? EncodingTable(string? name) => name switch
    {
        "WinAnsiEncoding" => FontTables.WinAnsiEncoding,
        "MacRomanEncoding" => FontTables.MacRomanEncoding,
        "MacExpertEncoding" => FontTables.MacExpertEncoding,
        "StandardEncoding" => FontTables.StandardEncoding,
        _ => null,
    };
}

/// <summary>Simple font: Type1, MMType1, TrueType — one byte per code (ISO 32000-2, 9.6).</summary>
internal sealed partial class SimpleFont : PdfFont
{
    private readonly string?[] _names = new string?[256];
    private readonly double[] _widths = new double[256];
    private readonly bool[] _hasWidth = new bool[256];
    private readonly double _missingWidth;
    private readonly FontProgram _program;
    private readonly bool _symbolic;
    private readonly bool _stretch;
    /// <summary>A standard 14 font drawn with the bundled substitute: widths it lacks are the standard ones, in whole thousandths (FG 884).</summary>
    private readonly bool _standardMetrics;
    private readonly bool _dingbats;
    private readonly bool _namesFromPdf;
    private readonly bool _hasBaseEncoding;
    private readonly int _flags;
    private readonly CMap? _toUnicode;

    public SimpleFont(PdfDictionary dict, PdfDocument doc) : base(dict.GetName(PdfName.Get("BaseFont"))?.Value ?? "")
    {
        var descriptor = dict.GetDictionary(PdfName.Get("FontDescriptor"));
        var flags = (int)(descriptor?.GetInteger(PdfName.Get("Flags")) ?? 0);
        _flags = flags;
        ReadMetrics(descriptor);
        _toUnicode = ReadToUnicode(dict, doc);

        var embedded = FontProgram.Embedded(descriptor, doc);
        var (file, isSymbol, isDingbats) = StandardFonts.Choose(BaseFont, descriptor);
        _program = embedded ?? FontProgram.Substitute(file);
        _dingbats = embedded is null && isDingbats;
        _symbolic = SymbolicFlag(flags, dict) || isSymbol || isDingbats;

        // encoding: base (name or BaseEncoding) and Differences
        string[]? baseTable = null;
        var differences = new string?[256];
        var hasDifferences = false;
        switch (dict.Get(PdfName.Get("Encoding")))
        {
            case PdfName n:
                baseTable = EncodingTable(n.Value);
                break;
            case PdfDictionary d:
                baseTable = EncodingTable(d.GetName(PdfName.Get("BaseEncoding"))?.Value);
                if (d.GetArray(PdfName.Get("Differences")) is { } diff)
                {
                    var code = 0;
                    foreach (var item in diff)
                    {
                        if (item.AsNumber() is { } number) code = (int)number;
                        else if (item is PdfName glyph && code is >= 0 and < 256)
                        {
                            differences[code++] = glyph.Value;
                            hasDifferences = true;
                        }
                    }
                }
                break;
        }
        _hasBaseEncoding = baseTable is not null;
        if (baseTable is null)
        {
            if (embedded is null && isSymbol) baseTable = FontTables.SymbolSetEncoding;
            else if (embedded is null && isDingbats) baseTable = FontTables.ZapfDingbatsEncoding;
            else if (embedded is { Type1: not null } || embedded is { Cff: not null, IsTrueType: false }) baseTable = null;   // built-in encoding
            // nonsymbolic: StandardEncoding (ISO 32000-2, Table 112), a TrueType font WinAnsi as widely used readers (No. 571, E-11). Without an
            // embedded program the font's /Subtype decides, not the substitute the reader draws with — a TrueType file for every standard font,
            // so Helvetica read 0x27 as ' instead of ’ (docs/FEHLERGEDAECHTNIS.md, No. 769)
            else if (!_symbolic)
                baseTable = (embedded is not null ? _program.IsTrueType : dict.GetName(PdfName.Subtype)?.Value == "TrueType")
                    ? FontTables.WinAnsiEncoding : FontTables.StandardEncoding;
        }
        for (var c = 0; c < 256; c++)
        {
            var name = differences[c] ?? (baseTable is { } t && t[c].Length > 0 ? t[c] : null);
            _names[c] = name;
        }
        _namesFromPdf = baseTable is not null || hasDifferences;

        // widths (1/1000 em)
        var first = (int)(dict.GetInteger(PdfName.Get("FirstChar")) ?? 0);
        if (dict.GetArray(PdfName.Get("Widths")) is { } widths)
            for (var i = 0; i < widths.Count; i++)
            {
                var c = first + i;
                if (c is < 0 or > 255 || widths.GetNumber(i) is not { } w) continue;
                _widths[c] = w / 1000;
                _hasWidth[c] = true;
            }
        _missingWidth = (descriptor?.GetNumber(PdfName.Get("MissingWidth")) ?? 0) / 1000;
        _stretch = embedded is null && !StandardFonts.IsStandard14(BaseFont);
        _standardMetrics = embedded is null && StandardFonts.HasStandardMetrics(BaseFont);
    }

    private SimpleFont(string baseFont) : base(baseFont)
    {
        _program = FontProgram.Substitute("LiberationSans-Regular.ttf");
        Array.Copy(FontTables.StandardEncoding, _names, 256);
        _namesFromPdf = true;
    }

    /// <summary>Substitute when the font cannot be read at all, so that the text at least stays visible.</summary>
    public static SimpleFont Fallback(PdfDictionary dict) => new(dict.GetName(PdfName.Get("BaseFont"))?.Value ?? "");

    protected override PdfGlyph Load(uint code, int length)
    {
        var c = (int)(code & 0xFF);
        var name = _names[c];
        var (outline, programWidth, glyphName) = Select(c, name);
        var toText = _program.FontMatrix;
        var width = _hasWidth[c] ? _widths[c]
            : programWidth > 0 ? (_standardMetrics ? Math.Round(programWidth * 1000) / 1000 : programWidth)
            : _missingWidth;

        // stretch the substitute to the width of the actual font (not for the standard 14 fonts,
        // whose substitutes already have the same widths)
        if (_stretch && _hasWidth[c] && programWidth > 0)
        {
            var sx = Math.Clamp(_widths[c] / programWidth, 0.5, 2);
            if (Math.Abs(sx - 1) > 0.02) toText *= Matrix.Scale(sx, 1);
        }

        var text = _toUnicode?.ToUnicode(code, 1)
                   ?? (name is not null ? GlyphNames.ToUnicode(name, _dingbats) : null)
                   ?? (glyphName is not null ? GlyphNames.ToUnicode(glyphName, _dingbats) : null)
                   ?? (!_symbolic && c >= 32 ? ((char)c).ToString() : null);
        return new PdfGlyph(outline, toText, width, text);
    }

    /// <summary>With /Widths only the codes that have a width (a subset lists only its characters), otherwise all 256.</summary>
    protected override IEnumerable<(uint Code, int Length)> CandidateCodes()
    {
        var any = Array.IndexOf(_hasWidth, true) >= 0;
        for (uint c = 0; c < 256; c++)
            if (!any || _hasWidth[c]) yield return (c, 1);
    }

    internal override IReadOnlyList<int> TrueTypeGlyphs(uint code, int length)
    {
        if (!_program.IsEmbedded || !_program.IsTrueType || _program.OpenType is not { } ot) return [];
        var c = (int)(code & 0xFF);
        var name = _names[c];
        var found = new List<int>(8);
        void Add(int g)
        {
            if (g > 0 && g < ot.GlyphCount) found.Add(g);
        }
        // the glyph the display draws first: the ways below are a superset for usual fonts, but its fallbacks reach further (every cmap with
        // the raw code); a shown glyph missing here was emptied out of the program after a redaction (docs/FEHLERGEDAECHTNIS.md, No. 382).
        // Also glyph 0, which the display draws for a code with a width but no glyph: emptied, a huge one of a damaged font became small and
        // its character slid under a bar (No. 386)
        if (DrawnGlyph(ot, c, name) is >= 0 and var drawn && drawn < ot.GlyphCount) found.Add(drawn);
        if (name is not null)
        {
            if (GlyphNames.CodePoint(name, _dingbats) is > 0 and var cp) Add(ot.GlyphForUnicode(cp));
            if (FontTables.MacOsRomanCode(name) is >= 0 and var mac) Add(ot.Lookup(1, 0, mac));
            Add(ot.GlyphByName(name));
        }
        foreach (var prefix in (ReadOnlySpan<int>)[0, 0xF000, 0xF100, 0xF200]) Add(ot.Lookup(3, 0, prefix + c));
        Add(ot.Lookup(1, 0, c));
        Add(ot.GlyphForUnicode(c));
        if (ot.CmapSubtables.Count == 0) Add(c);
        return found;
    }

    internal override (IReadOnlyList<int> Gids, IReadOnlyList<string> Names) ProgramGlyphs(uint code, int length)
    {
        if (!_program.IsEmbedded) return ([], []);
        var c = (int)(code & 0xFF);
        var name = _names[c];
        var gids = new List<int>(4);
        var names = new List<string>(2);
        if (_program.Type1 is { } t1)
        {
            if (name is not null && t1.HasGlyph(name)) names.Add(name);
            if (t1.Encoding[c] is { Length: > 0 } builtin) names.Add(builtin);
            return (gids, names);
        }
        if (_program.Cff is { } cff && !_program.IsTrueType)
        {
            void Add(int g)
            {
                if (g > 0 && g < cff.GlyphCount) gids.Add(g);
            }
            if (cff.IsCidKeyed) Add(cff.GlyphByCid(c));
            if (name is not null) Add(cff.GlyphByName(name));
            Add(cff.Encoding[c]);
            if (name is not null && _program.OpenType is { } otf && GlyphNames.CodePoint(name) is > 0 and var cp) Add(otf.GlyphForUnicode(cp));
            return (gids, names);
        }
        return (TrueTypeGlyphs(code, length), names);
    }

    /// <summary>Finds the glyph in the font program (ISO 32000-2, 9.6.5; 9.6.6.4 for TrueType).</summary>
    private (GlyphOutline Outline, double Width, string? GlyphName) Select(int code, string? name)
    {
        var scale = _program.FontMatrix.A;
        if (_program.Type1 is { } t1)
        {
            var n = name is not null && t1.HasGlyph(name) ? name : t1.Encoding[code] is { Length: > 0 } builtin ? builtin : name ?? ".notdef";
            return (t1.Outline(n), t1.AdvanceWidth(n) * scale, n);
        }

        if (_program.Cff is { } cff && !_program.IsTrueType)
        {
            var gid = -1;
            if (cff.IsCidKeyed) gid = cff.GlyphByCid(code);
            if (gid <= 0 && name is not null) gid = cff.GlyphByName(name);
            if (gid <= 0 && (!_namesFromPdf || name is null)) gid = cff.Encoding[code];
            if (gid <= 0 && name is not null && _program.OpenType is { } otf && GlyphNames.CodePoint(name) is > 0 and var cp) gid = otf.GlyphForUnicode(cp);
            if (gid <= 0 && cff.Encoding[code] > 0) gid = cff.Encoding[code];
            return gid > 0 ? (cff.Outline(gid), cff.AdvanceWidth(gid) * scale, cff.GlyphName(gid)) : (GlyphOutline.Empty, 0, null);
        }

        if (_program.OpenType is { } ot)
        {
            var gid = DrawnGlyph(ot, code, name);
            return gid < 0 ? (GlyphOutline.Empty, 0, null) : (ot.Outline(gid), ot.AdvanceWidth(gid) * scale, ot.GlyphName(gid));
        }
        return (GlyphOutline.Empty, 0, null);
    }

    /// <summary>
    /// The Symbolic bit decides (ISO 32000-2, 9.8.2: Symbolic and Nonsymbolic both set is an error, a reader checks the Symbolic bit) —
    /// unless both are set and the font brings an /Encoding: a symbolic TrueType font has none (9.6.5.4), so the producer meant the names
    /// (test file issue20232: code 71 named Ccedilla, the (3,0) cmap keyed by Latin-1 codes; the code itself finds an empty glyph). As before
    /// then (docs/FEHLERGEDAECHTNIS.md, No. 571).
    /// </summary>
    internal static bool SymbolicFlag(int flags, PdfDictionary font) =>
        (flags & 4) != 0 && ((flags & 32) == 0 || font.Get(PdfName.Get("Encoding")) is null or PdfNull);

    internal override int DrawnGlyphWith(OpenTypeFont program, uint code, int length)
    {
        if (!_program.IsEmbedded || !_program.IsTrueType) return int.MinValue;
        var c = (int)(code & 0xFF);
        return DrawnGlyph(program, c, _names[c]) is >= 0 and var g && g < program.GlyphCount ? g : -1;
    }

    /// <summary>The glyph a TrueType or OpenType program shows for a code as drawn (with the fallbacks of widely used readers); -1 if none is drawn.</summary>
    private int DrawnGlyph(OpenTypeFont ot, int code, string? name)
    {
        var gid = 0;
        // symbolic with a (3,0) cmap: the code first (ISO 32000-2, 9.6.5.4, as in widely used readers). Test file canvas: /Differences names
        // "space", but the (1,0) cmap maps raw codes, so going through the MacRoman position would give a wrong glyph
        if (_symbolic && ot.HasCmap(3, 0))
            foreach (var prefix in (ReadOnlySpan<int>)[0xF000, 0, 0xF100, 0xF200])
                if (ot.Lookup(3, 0, prefix + code) is > 0 and var g) { gid = g; break; }
        if (gid <= 0 && name is not null && (!_symbolic || _namesFromPdf))
        {
            var cp = GlyphNames.CodePoint(name, _dingbats);
            if (cp > 0 && ot.HasCmap(3, 1) || cp > 0 && ot.HasCmap(0, 3) || cp > 0 && ot.HasCmap(3, 10)) gid = ot.GlyphForUnicode(cp);
            if (gid <= 0 && ot.HasCmap(1, 0) && FontTables.MacOsRomanCode(name) is >= 0 and var mac) gid = ot.Lookup(1, 0, mac);
            if (gid <= 0) gid = Math.Max(0, ot.GlyphByName(name));
            if (gid <= 0 && !_program.IsEmbedded && _toUnicode?.ToUnicode((uint)code, 1) is { Length: > 0 } text) gid = ot.GlyphForUnicode(char.ConvertToUtf32(text, 0));
        }
        if (gid <= 0 && !_program.IsEmbedded)
        {
            // a code without a glyph name in a font that is not embedded (StandardEncoding leaves 0x7F–0xA0 and others empty): the raw code means
            // nothing in the substitute program — its (1,0) cmap drew 0x9B as „õ“. /ToUnicode first, as widely used readers, then the code as
            // Latin-1 (special rule A85; docs/FEHLERGEDAECHTNIS.md, No. 769)
            // white space (space, tab, line feed, carriage return, no-break space) as the space, also where the file names it .notdef: it
            // keeps its width (test file issue4304: /Differences [32 /.notdef] without /Widths — the words ran together; test file bug_white_space:
            // a tab between words); any other code the file names .notdef draws nothing (test file issue11403 — before, a substitute glyph)
            var space = code is 32 or 9 or 10 or 13 or 0xA0;
            if (name == ".notdef" && !space) return -1;
            if (!space && _toUnicode?.ToUnicode((uint)code, 1) is { Length: > 0 } text) gid = ot.GlyphForUnicode(char.ConvertToUtf32(text, 0));
            if (gid <= 0) gid = ot.GlyphForUnicode(space ? 0x20 : code);
            return gid <= 0 ? -1 : gid;
        }
        if (gid <= 0)
        {
            if (ot.HasCmap(3, 0))
                foreach (var prefix in (ReadOnlySpan<int>)[0, 0xF000, 0xF100, 0xF200])
                    if (ot.Lookup(3, 0, prefix + code) is > 0 and var g) { gid = g; break; }
            if (gid <= 0 && ot.HasCmap(1, 0)) gid = Math.Max(0, ot.Lookup(1, 0, code));
            if (gid <= 0 && !_program.IsEmbedded && _toUnicode?.ToUnicode((uint)code, 1) is { Length: > 0 } text) gid = ot.GlyphForUnicode(char.ConvertToUtf32(text, 0));
            if (gid <= 0 && (ot.HasCmap(3, 1) || !_program.IsEmbedded)) gid = ot.GlyphForUnicode(code);
            if (gid <= 0 && ot.CmapSubtables.Count == 0 && code < ot.GlyphCount) gid = code;   // broken subset without a cmap
            // otherwise every existing cmap with the raw code, as in common font engines (issue5501: only a (0,0) cmap, shifted codes;
            // without this step the characters would appear as placeholder boxes)
            if (gid <= 0)
                foreach (var (platform, encoding) in ot.CmapSubtables)
                    if (ot.Lookup(platform, encoding, code) is > 0 and var g) { gid = g; break; }
        }
        // Nothing found, but the PDF gives the code a width: draw glyph 0 as common PDF readers do (issue8234:
        // the cmap maps "ä" to glyph 0). Without a width it stays empty; otherwise placeholder boxes would appear
        if (gid <= 0 && _program.IsEmbedded && code is >= 0 and < 256 && _hasWidth[code] && _widths[code] > 0) return 0;
        return gid <= 0 ? -1 : gid;
    }

    /// <summary>
    /// PDF/A (rebuilding or embedding a font): the TrueType program the display draws with and, per code, the glyph it draws (-1 for none).
    /// Null if the font has no embedded TrueType program — with <paramref name="substitute"/> also the substitute of a font that is not embedded.
    /// </summary>
    internal (OpenTypeFont Program, Func<int, int> Glyph)? DrawnTrueType(bool substitute = false) =>
        _program is { IsTrueType: true, OpenType: { } ot } && (_program.IsEmbedded || substitute && !_stretch)
            ? (ot, code => DrawnGlyph(ot, code & 0xFF, _names[code & 0xFF]))
            : null;
}

/// <summary>Composite font (Type0) with a CIDFont (ISO 32000-2, 9.7): codes are mapped to CIDs through a CMap.</summary>
internal sealed partial class CompositeFont : PdfFont
{
    private readonly CMap _cmap;
    private readonly CMap? _toUnicode;
    private readonly FontProgram? _program;
    private readonly bool _cidFontType2;
    private readonly byte[]? _cidToGid;
    private readonly ushort[]? _glyphOrder;
    private readonly string? _ordering;
    private readonly double _defaultWidth = 1;
    // W and W2: ranges such as "1 65535 500" stay ranges (binary search); expanded they would be
    // tens of thousands of entries per line. Single values are listed individually in the file anyway.
    private readonly Dictionary<int, double> _widths = [];
    private readonly RangeMap<double> _widthRanges;
    private readonly double _defaultVy = 0.88, _defaultW1 = -1;
    private readonly Dictionary<int, (double W1, double Vx, double Vy)> _vertical = [];
    private readonly RangeMap<(double W1, double Vx, double Vy)> _verticalRanges;

    public CompositeFont(PdfDictionary dict, PdfDocument doc) : base(dict.GetName(PdfName.Get("BaseFont"))?.Value ?? "")
    {
        _toUnicode = ReadToUnicode(dict, doc);
        _cmap = dict.Get(PdfName.Get("Encoding")) switch
        {
            PdfName { Value: "Identity-V" } => CMap.Identity(vertical: true),
            PdfStream s => ParseCMap(s, doc),
            // predefined CMaps (CJK, bundled); unknown names: two bytes, CID = code
            PdfName n => PredefinedCMaps.Get(n.Value) ?? CMap.Identity(vertical: n.Value.EndsWith("-V", StringComparison.Ordinal)),
            _ => CMap.Identity(vertical: false),
        };
        Vertical = _cmap.WMode == 1;

        var cidFont = dict.GetArray(PdfName.Get("DescendantFonts"))?.Get(0) as PdfDictionary
                      ?? throw new PdfException("Type0 ohne DescendantFonts");
        var descriptor = cidFont.GetDictionary(PdfName.Get("FontDescriptor"));
        ReadMetrics(descriptor);
        _cidFontType2 = cidFont.GetName(PdfName.Subtype)?.Value == "CIDFontType2";
        _ordering = cidFont.GetDictionary(PdfName.Get("CIDSystemInfo"))?.Get(PdfName.Get("Ordering")) is PdfString ordering ? ordering.ToText() : null;
        _program = FontProgram.Embedded(descriptor, doc);
        if (_program is null)
        {
            var flags = (int)(descriptor?.GetInteger(PdfName.Get("Flags")) ?? 0);
            var bold = (flags & 1 << 18) != 0 || (descriptor?.GetNumber(PdfName.Get("FontWeight")) ?? 0) >= 600;
            var italic = (flags & 1 << 6) != 0 || Math.Abs(descriptor?.GetNumber(PdfName.Get("ItalicAngle")) ?? 0) > 1;
            var cjk = PredefinedCMaps.IsCjk(_ordering);
            // Chinese, Japanese, Korean: Noto CJK from the folder next to the program; if it is missing, the user interface reports it
            if (cjk && CjkFonts.Get(_ordering!, CjkFonts.IsSerif(BaseFont, flags)) is { } noto) _program = FontProgram.Substitute(noto);
            // Non-embedded TrueType CID font: the codes are usually glyph IDs of the Windows font. If that font is installed, it is
            // used itself; otherwise the glyph order of the family translates the IDs into characters for the substitute.
            // Not for CJK orderings: their CIDs are not glyph IDs of a Windows font
            else if (!cjk && _cidFontType2 && SystemFonts.Find(BaseFont, bold, italic) is { } system) _program = FontProgram.System(system);
            else
            {
                var (file, _, _) = StandardFonts.Choose(BaseFont, descriptor);
                _program = file.EndsWith(".ttf", StringComparison.Ordinal) ? FontProgram.Substitute(file) : FontProgram.Substitute("LiberationSans-Regular.ttf");
                if (cjk) doc.ReportMissingCjkFonts();
                else if (_cidFontType2) _glyphOrder = GlyphOrder.For(BaseFont);
            }
        }
        if (cidFont.GetStream(PdfName.Get("CIDToGIDMap")) is { } map)
        {
            try { _cidToGid = doc.GetStreamData(map); }
            catch (PdfException) { }
        }

        if (cidFont.GetNumber(PdfName.Get("DW")) is { } dw) _defaultWidth = dw / 1000;
        var widthRanges = new List<(int, int, double)>();
        if (cidFont.GetArray(PdfName.Get("W")) is { } w) ReadWidths(w, widthRanges);
        _widthRanges = new RangeMap<double>(widthRanges);
        if (cidFont.GetArray(PdfName.Get("DW2")) is { Count: >= 2 } dw2)
        {
            _defaultVy = (dw2.GetNumber(0) ?? 880) / 1000;
            _defaultW1 = (dw2.GetNumber(1) ?? -1000) / 1000;
        }
        var verticalRanges = new List<(int, int, (double, double, double))>();
        if (Vertical && cidFont.GetArray(PdfName.Get("W2")) is { } w2) ReadVerticalWidths(w2, verticalRanges);
        _verticalRanges = new RangeMap<(double W1, double Vx, double Vy)>(verticalRanges);
    }

    /// <summary>
    /// An embedded CMap (ISO 32000-2, 9.7.5.3). /UseCMap in its dictionary — the name of a predefined CMap or a stream with another CMap
    /// (Table 118) — supplies the mappings it does not repeat: through its "usecmap" operator, or added after it if the file has none. Before,
    /// only predefined names were resolved and the entry was not read: codes only the referenced CMap maps became CID 0
    /// (docs/FEHLERGEDAECHTNIS.md, No. 772). Streams at most four levels deep, never itself.
    /// </summary>
    private static CMap ParseCMap(PdfStream stream, PdfDocument doc)
    {
        try
        {
            var cmap = ParseUsing(stream, doc, 0);
            if (stream.Dictionary.GetInteger(PdfName.Get("WMode")) == 1 && cmap.WMode == 0)
                return CMap.Parse(doc.GetStreamData(stream), _ => CMap.Identity(true));
            return cmap;
        }
        catch (PdfException)
        {
            return CMap.Identity(false);
        }
    }

    /// <summary>A CMap stream with what its /UseCMap supplies (see <see cref="ParseCMap"/>); also for ToUnicode CMaps (9.10.3).</summary>
    internal static CMap ParseUsing(PdfStream stream, PdfDocument doc, int depth)
    {
        var parent = depth < 4 ? stream.Dictionary.Get(PdfName.Get("UseCMap")) switch
        {
            PdfName name => PredefinedCMaps.Get(name.Value),
            PdfStream other when !ReferenceEquals(other, stream) => ParseUsing(other, doc, depth + 1),
            _ => null,
        } : null;
        var included = false;
        var cmap = CMap.Parse(doc.GetStreamData(stream), name =>
        {
            if (PredefinedCMaps.Get(name) is { } predefined) return predefined;
            included = parent is not null;
            return parent;
        });
        if (parent is not null && !included && !ReferenceEquals(parent, cmap)) cmap.Include(parent);
        return cmap;
    }

    /// <summary>
    /// /W (ISO 32000-2, 9.7.4.3): groups of „c [w …]“ and „c_first c_last w“. A CID given more than once takes its first width — before, a later
    /// list overrode an earlier list or range (docs/FEHLERGEDAECHTNIS.md, No. 770). The lookup takes the lists first, then the ranges in
    /// order; a list entry that an earlier range already covers is therefore left out.
    /// </summary>
    private void ReadWidths(PdfArray w, List<(int, int, double)> ranges)
    {
        var listed = new Dictionary<int, (double Width, int Group)>();
        var grouped = new List<(int First, int Last, int Group)>();
        for (int i = 0, group = 0; i < w.Count; group++)
        {
            if (w.GetNumber(i) is not { } first) { i++; continue; }
            if (w.Get(i + 1) is PdfArray list)
            {
                for (var k = 0; k < list.Count; k++)
                    if (list.GetNumber(k) is { } width) listed.TryAdd((int)first + k, (width / 1000, group));
                i += 2;
            }
            else if (w.GetNumber(i + 1) is { } last && w.GetNumber(i + 2) is { } width)
            {
                ranges.Add(((int)first, (int)last, width / 1000));
                grouped.Add(((int)first, (int)last, group));
                i += 3;
            }
            else break;
        }
        var earlier = new RangeMap<int>(grouped);
        foreach (var (cid, (width, group)) in listed)
            if (!(earlier.TryGet(cid, out var rangeGroup) && rangeGroup < group)) _widths[cid] = width;
    }

    private void ReadVerticalWidths(PdfArray w2, List<(int, int, (double, double, double))> ranges)
    {
        for (var i = 0; i < w2.Count;)
        {
            if (w2.GetNumber(i) is not { } first) { i++; continue; }
            if (w2.Get(i + 1) is PdfArray list)
            {
                for (var k = 0; k + 2 < list.Count; k += 3)
                    _vertical[(int)first + k / 3] = ((list.GetNumber(k) ?? -1000) / 1000, (list.GetNumber(k + 1) ?? 0) / 1000, (list.GetNumber(k + 2) ?? 880) / 1000);
                i += 2;
            }
            else if (w2.GetNumber(i + 1) is { } last && i + 4 < w2.Count)
            {
                var v = ((w2.GetNumber(i + 2) ?? -1000) / 1000, (w2.GetNumber(i + 3) ?? 0) / 1000, (w2.GetNumber(i + 4) ?? 880) / 1000);
                ranges.Add(((int)first, (int)last, v));
                i += 5;
            }
            else break;
        }
    }

    public override int ReadCode(ReadOnlySpan<byte> s, int pos, out uint code) => _cmap.ReadCode(s, pos, out code);

    private double Width(int cid)
    {
        if (_widths.TryGetValue(cid, out var w)) return w;
        return _widthRanges.TryGet(cid, out w) ? w : _defaultWidth;
    }

    protected override PdfGlyph Load(uint code, int length)
    {
        var cid = _cmap.ToCid(code, length);
        var width = Width(cid);
        // without ToUnicode: for CJK collections the character of the CID (Adobe's UCS2 mappings), for the substitute and for copying
        var text = _toUnicode?.ToUnicode(code, length) ?? PredefinedCMaps.Text(_ordering, cid);
        var (outline, toText, glyphText) = Select(cid, text);
        text ??= glyphText;

        var glyph = new PdfGlyph(outline, toText, width, text);
        if (!Vertical) return glyph;
        var (w1, vx, vy) = _vertical.TryGetValue(cid, out var v) || _verticalRanges.TryGet(cid, out v) ? v : (_defaultW1, width / 2, _defaultVy);
        return glyph with { VerticalAdvance = w1, OriginX = vx, OriginY = vy };
    }

    /// <summary>The codes from ToUnicode (without it, it is unknown which code shows which character).</summary>
    protected override IEnumerable<(uint Code, int Length)> CandidateCodes() => _toUnicode?.UnicodeCodes() ?? [];

    internal override IReadOnlyList<int> TrueTypeGlyphs(uint code, int length)
    {
        if (_program is not { IsEmbedded: true, IsTrueType: true, OpenType: { } ot }) return [];
        var cid = _cmap.ToCid(code, length);
        var gid = cid;
        if (_cidToGid is { } map && _cidFontType2) gid = MappedGid(map, cid);
        return gid >= 0 && gid < ot.GlyphCount ? [gid] : [];   // glyph 0 as well: the display draws it (Select; docs/FEHLERGEDAECHTNIS.md, No. 386)
    }

    internal override int Cid(uint code, int length) => _cmap.ToCid(code, length);

    internal override double CidWidth(int cid) => Width(cid) * 1000;

    /// <summary>Whether a code lies in a code space range of the font's CMap (<see cref="CMap.InCodespace"/>).</summary>
    internal bool InCodespace(uint code, int length) => _cmap.InCodespace(code, length);

    internal override (IReadOnlyList<int> Gids, IReadOnlyList<string> Names) ProgramGlyphs(uint code, int length)
    {
        if (_program is not { IsEmbedded: true } program) return ([], []);
        var cid = _cmap.ToCid(code, length);
        int Mapped(byte[] map) => MappedGid(map, cid);
        if (program.Cff is { } cff && !program.IsTrueType)
        {
            // as when drawing (Select), and the CID itself: either may be the glyph
            var number = _cidToGid is { } cffMap && (_cidFontType2 || !cff.IsCidKeyed) ? Mapped(cffMap) : cid;
            var gids = new List<int>(2);
            foreach (var g in (ReadOnlySpan<int>)[cff.IsCidKeyed ? cff.GlyphByCid(number) : number, cff.IsCidKeyed ? cff.GlyphByCid(cid) : cid])
                if (g > 0 && g < cff.GlyphCount) gids.Add(g);
            return (gids, []);
        }
        if (program.Type1 is { } t1)
        {
            var gid = _cidToGid is { } map ? Mapped(map) : cid;
            return ([], t1.GlyphNameAt(gid) is { } name ? [name] : []);
        }
        return (TrueTypeGlyphs(code, length), []);
    }

    /// <summary>The glyph of a CID through a CIDToGIDMap; 0 for a CID outside the map — also a negative one, which a damaged CMap can give.</summary>
    private static int MappedGid(byte[] map, int cid) => InMap(map.Length, cid) ? map[2 * cid] << 8 | map[2 * cid + 1] : 0;

    /// <summary>
    /// Whether a CIDToGIDMap of <paramref name="length"/> bytes holds an entry for <paramref name="cid"/> — the one rule for the display and
    /// for shortening the map after redacting: no negative CIDs, and the position computed without overflow (a CID beyond a billion made
    /// 2 · CID negative; docs/FEHLERGEDAECHTNIS.md, No. 404, 422).
    /// </summary>
    internal static bool InMap(int length, int cid) => cid >= 0 && 2L * cid + 1 < length;

    private (GlyphOutline Outline, Matrix ToText, string? Text) Select(int cid, string? text)
    {
        var program = _program!;
        var toText = program.FontMatrix;
        if (!program.IsEmbedded)
        {
            if (program.OpenType is not { } sub) return (GlyphOutline.Empty, toText, null);
            // substitute through the text (ToUnicode)
            // (if the substitute lacks the character: nothing, not its placeholder — CJK without the font folder)
            if (text is { Length: > 0 })
                return (sub.GlyphForUnicode(char.ConvertToUtf32(text, 0)) is > 0 and var g ? sub.Outline(g) : GlyphOutline.Empty, toText, null);
            // without text: glyph IDs of the installed font, otherwise through the glyph order of the family
            var number = _cidToGid is { } numbers && _cidFontType2 ? MappedGid(numbers, cid) : cid;
            if (program.IsSystem) return (sub.Outline(number), toText, ReverseCmap(sub, number));
            if (_glyphOrder is { } order && number > 0 && number < order.Length && order[number] > 0)
                return (sub.Outline(sub.GlyphForUnicode(order[number])), toText, ((char)order[number]).ToString());
            return (GlyphOutline.Empty, toText, null);
        }
        if (program.Cff is { } cff && !program.IsTrueType)
        {
            // CFF under CIDFontType2 with a CIDToGIDMap (bug1186827): the mapping applies here as well. If the CFF is CID-keyed, its
            // result is again a CID that only the charset of the CFF turns into a glyph, as in common font engines (issue15559).
            // Under CIDFontType0 the mapping is not actually intended, but other readers follow it there too (issue7901: name-keyed
            // OpenType CFF, garbled text without the mapping); there only for name-keyed CFF, not for CID-keyed CFF
            var number = _cidToGid is { } cffMap && (_cidFontType2 || !cff.IsCidKeyed) ? MappedGid(cffMap, cid) : cid;
            var gid = cff.IsCidKeyed ? cff.GlyphByCid(number) : number;
            return (cff.Outline(gid), toText, null);
        }
        if (program.OpenType is { } ot)
        {
            var gid = cid;
            if (_cidToGid is { } map && _cidFontType2)
                gid = MappedGid(map, cid);
            return (ot.Outline(gid), toText, ReverseCmap(ot, gid));
        }
        if (program.Type1 is { } t1)
        {
            // Type 1 as a CIDFont (issue11740): CID = glyph in the order of the CharStrings, as in widely used readers
            var gid = cid;
            if (_cidToGid is { } map) gid = MappedGid(map, cid);
            return t1.GlyphNameAt(gid) is { } name ? (t1.Outline(name), toText, null) : (GlyphOutline.Empty, toText, null);
        }
        return (GlyphOutline.Empty, toText, null);
    }

    /// <summary>
    /// PDF/A (rebuilding a font): the embedded TrueType program of a CIDFontType2 and, per CID, the glyph the display draws. Null if the font has
    /// no embedded TrueType program under CIDFontType2.
    /// </summary>
    internal (OpenTypeFont Program, Func<int, int> Glyph)? DrawnTrueType() =>
        _cidFontType2 && _program is { IsEmbedded: true, IsTrueType: true, OpenType: { } ot }
            ? (ot, cid => _cidToGid is { } map ? MappedGid(map, cid) : cid)
            : null;

    /// <summary>
    /// Text if ToUnicode is missing: the inverted cmap of the embedded font and, for a font with Arabic, the forms its shaping puts in place of a
    /// letter (<see cref="Core.Fonts.Layout.GlyphTexts"/>; docs/FEHLERGEDAECHTNIS.md, No. 920).
    /// </summary>
    private string? ReverseCmap(OpenTypeFont ot, int gid) =>
        _toUnicode is null && Core.Fonts.Layout.GlyphTexts.Of(ot).TryGetValue(gid, out var text) ? text : null;
}

/// <summary>
/// Ranges [First, Last] → value with binary search. If ranges overlap (which happens in
/// faulty files), the first one in file order wins, as with a linear search.
/// </summary>
internal sealed class RangeMap<T>
{
    private readonly (int First, int Last, T Value)[] _ranges;
    private readonly bool _overlapping;

    public RangeMap(List<(int First, int Last, T Value)> ranges)
    {
        _ranges = [.. ranges.Where(r => r.Last >= r.First)];
        // stable sort by start (OrderBy is stable, so ranges with the same start keep their file order)
        var sorted = _ranges.OrderBy(r => r.First).ToArray();
        for (var i = 1; i < sorted.Length; i++)
            if (sorted[i].First <= sorted[i - 1].Last) _overlapping = true;
        if (!_overlapping) _ranges = sorted;
    }

    public bool TryGet(int key, out T value)
    {
        var ranges = _ranges;
        if (_overlapping)
        {
            foreach (var r in ranges)
                if (key >= r.First && key <= r.Last) { value = r.Value; return true; }
        }
        else
        {
            int lo = 0, hi = ranges.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                ref readonly var r = ref ranges[mid];
                if (key < r.First) hi = mid - 1;
                else if (key > r.Last) lo = mid + 1;
                else { value = r.Value; return true; }
            }
        }
        value = default!;
        return false;
    }
}

/// <summary>Type 3 font (ISO 32000-2, 9.6.4): glyphs are small content streams.</summary>
internal sealed partial class Type3Font : PdfFont
{
    private readonly Matrix _fontMatrix;
    private readonly PdfDictionary? _charProcs;
    private readonly PdfDictionary? _resources;
    private readonly string?[] _names = new string?[256];
    private readonly string[]? _baseNames;
    private readonly double[] _widths = new double[256];
    private readonly double[] _glyphSpaceWidths = new double[256];
    private readonly PdfDocument _doc;
    private readonly CMap? _toUnicode;

    public Type3Font(PdfDictionary dict, PdfDocument doc) : base(dict.GetName(PdfName.Get("Name"))?.Value ?? "Type3")
    {
        _fontMatrix = Matrix.FromArray(dict.GetArray(PdfName.Get("FontMatrix"))) is { IsInvertible: true } m ? m : new Matrix(0.001, 0, 0, 0.001, 0, 0);
        _doc = doc;
        _charProcs = dict.GetDictionary(PdfName.Get("CharProcs"));
        _resources = dict.GetDictionary(PdfName.Resources);
        _toUnicode = ReadToUnicode(dict, doc);
        if (dict.Get(PdfName.Get("Encoding")) is PdfDictionary baseEncoding && baseEncoding.GetName(PdfName.Get("BaseEncoding")) is { } baseName)
            _baseNames = EncodingTable(baseName.Value);
        if (dict.Get(PdfName.Get("Encoding")) is PdfDictionary enc && enc.GetArray(PdfName.Get("Differences")) is { } diff)
        {
            var code = 0;
            foreach (var item in diff)
            {
                if (item.AsNumber() is { } number) code = (int)number;
                else if (item is PdfName glyph && code is >= 0 and < 256) _names[code++] = glyph.Value;
            }
        }
        var first = (int)(dict.GetInteger(PdfName.Get("FirstChar")) ?? 0);
        if (dict.GetArray(PdfName.Get("Widths")) is { } widths)
            for (var i = 0; i < widths.Count; i++)
                if (first + i is >= 0 and < 256 && widths.GetNumber(i) is { } w)
                {
                    _widths[first + i] = _fontMatrix.TransformVector(w, 0).X;   // glyph space → text space
                    _glyphSpaceWidths[first + i] = w;
                }
        // Height for selection and „Text schwärzen“: the glyphs' boxes (d1 of their procedures), otherwise the font's box, through the
        // font matrix — with the identity matrix a TeX font draws in text space directly, and 0.8/0.2 of the font size made boxes (and
        // bars) a fraction of the glyphs' height; some fonts carry a box of 1 unit only (docs/FEHLERGEDAECHTNIS.md, No. 149). Without
        // anything usable, the usual proportions.
        var height = Math.Abs(_fontMatrix.D) > 0 ? 1 : 0;
        Ascent = 0.8 * height;
        Descent = -0.2 * height;
        if (GlyphBox(dict, doc) is var (low, high))
        {
            Ascent = high;
            Descent = low;
        }
    }

    /// <summary>Vertical extent in text space (through the font matrix) of the glyphs' boxes from d1, or of /FontBBox; null if neither is usable.</summary>
    private (double Low, double High)? GlyphBox(PdfDictionary dict, PdfDocument doc)
    {
        double low = double.MaxValue, high = double.MinValue;
        void Add(double x0, double y0, double x1, double y1)
        {
            foreach (var (x, y) in (ReadOnlySpan<(double, double)>)[(x0, y0), (x1, y0), (x0, y1), (x1, y1)])
            {
                var (_, ty) = _fontMatrix.Transform(x, y);
                low = Math.Min(low, ty);
                high = Math.Max(high, ty);
            }
        }
        if (_charProcs is not null && ProcBounds(_charProcs, doc) is var (gx0, gy0, gx1, gy1)) Add(gx0, gy0, gx1, gy1);
        if (high <= low && dict.GetArray(PdfName.Get("FontBBox")) is { Count: 4 } box && box.GetNumber(0) is { } bx0 && box.GetNumber(1) is { } by0 &&
            box.GetNumber(2) is { } bx1 && box.GetNumber(3) is { } by1)
            Add(bx0, by0, bx1, by1);
        // a box much smaller or larger than the glyphs' advances is a placeholder or broken (a font with /FontBBox [0 0 1 -1] and widths
        // around 100); glyph units are free (a TeX font with the identity matrix has glyphs 100 units high at 0.12 Tf)
        var widest = _widths.Max(w => Math.Abs(w));
        var span = high - low;
        if (!(span > 1e-6) || !double.IsFinite(span) || widest > 0 && (span < widest * 0.05 || span > widest * 50)) return null;
        return (low, high);
    }

    /// <summary>
    /// The box around the glyphs' boxes from d1 (of the first 256 glyphs), in glyph space; null if none has one. A copy of the font with
    /// fewer glyphs (redaction) keeps it through a placeholder glyph, so that its height — and the boxes of the text left — stay
    /// (docs/FEHLERGEDAECHTNIS.md, No. 161).
    /// </summary>
    internal static (double X0, double Y0, double X1, double Y1)? ProcBounds(PdfDictionary charProcs, PdfDocument doc)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        var glyphs = 0;
        foreach (var (key, _) in charProcs.Entries)
        {
            if (++glyphs > 256) break;
            try
            {
                if (charProcs.GetStream(key) is not { } proc || proc.DataLength > 1 << 20) continue;
                var parser = new Content.ContentParser(doc.GetStreamData(proc));
                for (var ops = 0; ops < 4 && parser.Next() is { } op; ops++)
                    if (op == "d1" && parser.Operands.Count >= 6)
                    {
                        var o = parser.Operands;
                        if (o[2].Number != o[4].Number && o[3].Number != o[5].Number)   // an empty box says nothing
                        {
                            x0 = Math.Min(x0, Math.Min(o[2].Number, o[4].Number));
                            x1 = Math.Max(x1, Math.Max(o[2].Number, o[4].Number));
                            y0 = Math.Min(y0, Math.Min(o[3].Number, o[5].Number));
                            y1 = Math.Max(y1, Math.Max(o[3].Number, o[5].Number));
                        }
                        break;
                    }
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IndexOutOfRangeException)
            {
            }
        }
        return x1 > x0 && y1 > y0 && double.IsFinite(x1 - x0) && double.IsFinite(y1 - y0) ? (x0, y0, x1, y1) : null;
    }

    public override bool IsType3 => true;

    public override PdfDictionary? Type3Resources => _resources;

    /// <summary>A glyph of a Type 3 font shows something if its procedure paints (text editing with the file's font, phase 8.1).</summary>
    protected override bool ShowsInk(PdfGlyph glyph, uint code) => glyph.Procedure is not null && ProcedurePaints(code) && !TextGuessed(code);

    /// <summary>
    /// Whether the text of a code is only guessed from the code itself — no ToUnicode entry and no glyph name the glyph list knows (TeX pixel
    /// fonts name their glyphs „a65“). Searching and copying keep the guess; editing does not trust it: typed letters would be drawn with
    /// whatever glyph sits at that code (No. 611).
    /// </summary>
    /// <param name="code">The character code.</param>
    /// <returns>True if the text comes only from the code.</returns>
    internal bool TextGuessed(uint code)
    {
        var c = (int)(code & 0xFF);
        return _toUnicode?.ToUnicode(code, 1) is null && (_names[c] is not { } name || GlyphNames.ToUnicode(name) is null);
    }

    protected override PdfGlyph Load(uint code, int length)
    {
        var c = (int)(code & 0xFF);
        var name = _names[c];
        var proc = name is not null ? _charProcs?.GetStream(PdfName.Get(name)) : null;
        var text = _toUnicode?.ToUnicode(code, 1) ?? (name is not null ? GlyphNames.ToUnicode(name) : null)
                   ?? (c >= 32 ? ((char)c).ToString() : null);
        return new PdfGlyph(GlyphOutline.Empty, _fontMatrix, _widths[c], text, proc);
    }
}
